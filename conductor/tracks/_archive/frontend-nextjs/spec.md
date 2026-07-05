# Frontend Next.js — Specification

## Goal
Create a Next.js frontend application that replicates azoaweb4.com and provides a testing interface for the AZOA backend API, focusing on wallet registration and account management flows.

## Motivation
- Need a testing interface to validate the backend API functionality
- Want to replicate the azoaweb4.com design and user experience
- Need to test the Algorand and Solana devnet provider integrations
- Require a user-friendly interface for wallet registration and management

## Architecture
Next.js 14 with App Router, TypeScript, Tailwind CSS, and modern React patterns.

### New Files Structure
```
frontend-nextjs/
├── src/
│   ├── app/
│   │   ├── globals.css
│   │   ├── layout.tsx
│   │   └── page.tsx
│   ├── components/
│   │   ├── ui/
│   │   │   ├── button.tsx
│   │   │   ├── card.tsx
│   │   │   ├── input.tsx
│   │   │   └── label.tsx
│   │   ├── layout/
│   │   │   ├── header.tsx
│   │   │   └── footer.tsx
│   │   ├── auth/
│   │   │   ├── wallet-connect.tsx
│   │   │   └── wallet-selector.tsx
│   │   ├── identity/
│   │   │   ├── avatar-form.tsx
│   │   │   └── wallet-registration.tsx
│   │   └── blockchain/
│   │       ├── balance-display.tsx
│   │       ├── transaction-history.tsx
│   │       └── network-selector.tsx
│   ├── lib/
│   │   ├── api.ts
│   │   ├── types.ts
│   │   └── utils.ts
│   └── hooks/
│       ├── use-wallet.ts
│       └── use-avatar.ts
├── package.json
├── tailwind.config.ts
├── next.config.ts
└── tsconfig.json
```

## Key Features

### 1. Landing Page (Replicate azoaweb4.com)
- Hero section with "One API. All chains. Infinite possibilities."
- Principles section (Universal Interoperability, Zero Downtime, Permissionless)
- Ecosystem overview with supported blockchains
- Code examples for identity, NFT, and bridge operations

### 2. Identity Management
- Avatar creation form (username, email)
- Wallet registration for multiple chains (Algorand, Solana)
- Wallet connection and management interface

### 3. Wallet Management
- Multi-chain wallet support (Algorand, Solana)
- Balance display for each wallet
- Transaction history
- Network switching (devnet/testnet)

### 4. Testing Interface
- API endpoint testing for all backend operations
- Wallet registration flow testing
- Transaction simulation
- Provider connectivity testing

## API Integration
The frontend will integrate with the backend API using TypeScript interfaces:

```typescript
// src/lib/api.ts
export class AZOAApiClient {
  private baseUrl = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:5000';
  
  // Identity operations
  async createAvatar(data: { username: string; email: string }) {
    return await fetch(`${this.baseUrl}/api/avatar`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(data)
    });
  }
  
  async createWallet(data: { avatarId: string; chainType: string; address: string }) {
    return await fetch(`${this.baseUrl}/api/wallets`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(data)
    });
  }
  
  // Blockchain operations
  async getBalance(chain: string, address: string) {
    return await fetch(`${this.baseUrl}/api/blockchain/${chain}/balance/${address}`);
  }
}
```

## UI Components

### Wallet Connection
```typescript
// src/components/auth/wallet-connect.tsx
interface WalletConnectProps {
  onConnect: (wallet: WalletData) => void;
  availableChains: string[];
}

export function WalletConnect({ onConnect, availableChains }: WalletConnectProps) {
  const [selectedChain, setSelectedChain] = useState('');
  const [address, setAddress] = useState('');
  
  return (
    <Card>
      <h3>Connect Wallet</h3>
      <select value={selectedChain} onChange={(e) => setSelectedChain(e.target.value)}>
        {availableChains.map(chain => (
          <option key={chain} value={chain}>{chain}</option>
        ))}
      </select>
      <Input 
        placeholder="Wallet Address" 
        value={address}
        onChange={(e) => setAddress(e.target.value)}
      />
      <Button onClick={() => onConnect({ chain: selectedChain, address })}>
        Connect
      </Button>
    </Card>
  );
}
```

### Wallet Registration Form
```typescript
// src/components/identity/wallet-registration.tsx
interface WalletRegistrationProps {
  avatarId: string;
  onWalletRegistered: (wallet: Wallet) => void;
}

export function WalletRegistration({ avatarId, onWalletRegistered }: WalletRegistrationProps) {
  const [formData, setFormData] = useState({
    chainType: 'Solana',
    address: '',
    label: '',
    isDefault: false
  });
  
  return (
    <Card>
      <h3>Register New Wallet</h3>
      <form onSubmit={async (e) => {
        e.preventDefault();
        const response = await api.createWallet({
          avatarId,
          ...formData
        });
        onWalletRegistered(response.data);
      }}>
        {/* Form fields for chain type, address, label */}
        <Button type="submit">Register Wallet</Button>
      </form>
    </Card>
  );
}
```

## State Management
- React hooks for local state
- Context API for global state (wallets, avatar)
- Zustand for complex state if needed

## Styling
- Tailwind CSS for utility-first styling
- Responsive design for mobile and desktop
- Dark/light mode support
- Consistent color scheme matching azoaweb4.com

## Environment Configuration
```env
NEXT_PUBLIC_API_URL=http://localhost:5000
NEXT_PUBLIC_ALGORAND_DEVNET_URL=https://testnet-api.algonode.cloud
NEXT_PUBLIC_SOLANA_DEVNET_URL=https://api.devnet.solana.com
```

## Acceptance Criteria
- [ ] Landing page replicates azoaweb4.com design and content
- [ ] Avatar creation form works with backend API
- [ ] Wallet registration supports Algorand and Solana devnet
- [ ] Wallet connection and display functionality
- [ ] Balance display for connected wallets
- [ ] Transaction history display
- [ ] Network switching functionality
- [ ] Responsive design works on mobile and desktop
- [ ] TypeScript types for all API responses
- [ ] Error handling for API failures
- [ ] Loading states for async operations