# AZOA Frontend

A comprehensive Next.js frontend for testing blockchain providers with real connectivity to Algorand and Solana devnet networks.

## 🚀 Features

### Core Functionality
- **Real Blockchain Connectivity** - Test with actual Algorand and Solana devnet providers
- **Multi-Chain Support** - Switch between Algorand and Solana networks
- **Live Transaction Monitoring** - Track transaction status in real-time
- **Comprehensive Testing Suite** - Test all blockchain provider functions

### User Authentication
- **User Registration** - Create new accounts with validation
- **User Login** - Secure authentication with session management
- **User Logout** - Complete session cleanup
- **Session Persistence** - Remember user across page reloads
- **Error Handling** - Graceful handling of authentication errors

### Dashboard Components
- **Blockchain Dashboard** - Real-time network status and provider health
- **Wallet Manager** - Multi-wallet support with balance and token portfolio
- **Transaction History** - Complete transaction tracking with status monitoring
- **Avatar NFT Dashboard** - Digital identity with holon integration
- **Testing Interface** - Comprehensive testing suite for all provider functions

## 🛠️ Technology Stack

- **Next.js 14** - React framework with TypeScript
- **Tailwind CSS** - Utility-first styling
- **Axios** - HTTP client for API communication
- **TypeScript** - Type-safe development
- **React Hooks** - Modern React state management
- **Responsive Design** - Mobile-friendly interface

## 📦 Installation

### Prerequisites
- Node.js 18+
- npm or yarn

### Setup
```bash
# Clone the repository
git clone https://github.com/your-username/azoa-frontend.git
cd azoa-frontend

# Install dependencies
npm install

# Start development server
npm run dev
```

### Environment Variables
Create a `.env.local` file in the root directory:
```env
NEXT_PUBLIC_API_URL=http://localhost:5000
```

## 🔧 Development

### Available Scripts
```bash
# Development server
npm run dev

# Build for production
npm run build

# Start production server
npm start

# Linting
npm run lint

# Type checking
npx tsc --noEmit
```

### Project Structure
```
src/
├── app/                 # Next.js app directory
│   ├── globals.css     # Global styles
│   ├── layout.tsx       # Root layout
│   └── page.tsx        # Home page
├── components/          # React components
│   ├── AuthWrapper.tsx
│   ├── AvatarNFTDashboard.tsx
│   ├── BlockchainDashboard.tsx
│   ├── TestInterface.tsx
│   └── TransactionHistory.tsx
│   └── WalletManager.tsx
└── lib/
    ├── api.ts          # API client and types
    └── auth-simple.tsx # Authentication system
```

## 🔐 Authentication System

### Features
- **Secure Login/Register** - Form validation with error handling
- **Session Management** - localStorage persistence
- **Loading States** - Prevents UI flicker during authentication
- **Error Handling** - Graceful error messages
- **Input Validation** - Password strength requirements

### Usage
```typescript
import { useAuth } from '@/lib/auth-simple'

function MyComponent() {
  const { user, isAuthenticated, login, register, logout } = useAuth()
  
  // Use authentication functions
}
```

## 🌐 API Integration

### Endpoints
- **Balance Retrieval** - Get native and token balances
- **Address Validation** - Validate blockchain addresses
- **Transaction Status** - Monitor transaction progress
- **Token Metadata** - Fetch token information
- **Chain Info** - Get network status

### Example Usage
```typescript
import { apiClient } from '@/lib/api'

// Get balance
const response = await apiClient.getBalance({
  address: '7J6ZZGF2UPNKKBCJA4DHFKVL6LXGKKDQM6KX4YZ5J5H5F7ZJGX6W4PUJJY'
})

// Validate address
const response = await apiClient.validateAddress({
  address: '7J6ZZGF2UPNKKBCJA4DHFKVL6LXGKKDQM6KX4YZ5J5H5F7ZJGX6W4PUJJY'
})
```

## 🎨 UI Components

### Design System
- **Blockchain Theme** - Cryptocurrency-inspired color scheme
- **Responsive Design** - Works on all device sizes
- **Loading States** - Smooth loading animations
- **Error Handling** - User-friendly error messages
- **Status Indicators** - Color-coded status displays

### Components
- **AuthWrapper** - Main authentication wrapper
- **BlockchainDashboard** - Network status display
- **WalletManager** - Wallet management interface
- **TransactionHistory** - Transaction tracking
- **TestInterface** - Testing suite interface

## 🚀 Deployment

### Vercel (Recommended)
1. Push code to GitHub
2. Connect repository to Vercel
3. Configure environment variables
4. Deploy automatically

### Netlify
1. Push code to Git repository
2. Connect to Netlify
3. Configure build settings
4. Set environment variables

### Docker
```dockerfile
FROM node:18-alpine
WORKDIR /app
COPY package*.json ./
RUN npm ci --only=production
COPY . .
RUN npm run build
EXPOSE 3000
CMD ["npm", "start"]
```

### Traditional Server
```bash
npm run build
npm start
# Use PM2 for process management
pm2 start ecosystem.config.js
```

## 🧪 Testing

### Authentication Tests
- User registration flow
- User login flow
- User logout flow
- Error handling
- Session persistence

### Component Tests
- All components render correctly
- State management works
- API integration functions
- Error handling displays
- Loading states show properly

### Build Verification
- TypeScript compilation successful
- No linting errors
- All imports resolved
- Static generation works
- Dynamic imports functional

## 🔒 Security Features

### Authentication Security
- Password validation
- Session management
- XSS protection
- CSRF protection
- Input sanitization

### API Security
- HTTPS required
- Environment variables
- Error handling
- Rate limiting ready
- Input validation

### Data Protection
- No sensitive data in localStorage
- Secure API calls
- No hardcoded credentials
- Proper error messages

## 📊 Performance Optimizations

### Code Splitting
- Dynamic imports for components
- Route-based splitting
- Lazy loading for heavy components

### Bundle Optimization
- Tree shaking enabled
- Minification active
- Compression enabled
- Caching headers set

### SEO Optimization
- Meta tags configured
- Open Graph tags included
- Structured data ready
- Sitemap generation possible

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Test thoroughly
5. Submit a pull request

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- **Next.js** - React framework
- **Tailwind CSS** - Utility-first CSS framework
- **Axios** - HTTP client
- **TypeScript** - Type-safe JavaScript

## 📞 Support

For support, please open an issue in the GitHub repository or contact the development team.

---

**Built with ❤️ using Next.js and TypeScript**