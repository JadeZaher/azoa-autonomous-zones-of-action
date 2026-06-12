'use client'

import { useState } from 'react'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'

export default function SelectDebug() {
  const [value, setValue] = useState('')

  return (
    <div className="p-8">
      <h1 className="text-2xl font-bold mb-6">Select Component Debug</h1>
      
      <div className="space-y-6">
        <div>
          <label className="block text-sm font-medium mb-2">Basic Select</label>
          <Select value={value} onValueChange={(v) => setValue(v ?? '')}>
            <SelectTrigger className="w-[200px]">
              <SelectValue placeholder="Select a chain" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="algorand">Algorand</SelectItem>
              <SelectItem value="solana">Solana</SelectItem>
              <SelectItem value="ethereum">Ethereum</SelectItem>
            </SelectContent>
          </Select>
          <p className="mt-2 text-sm text-muted-foreground">Selected: {value}</p>
        </div>

        <div>
          <label className="block text-sm font-medium mb-2">With Icons</label>
          <Select value={value} onValueChange={(v) => setValue(v ?? '')}>
            <SelectTrigger className="w-[200px]">
              <SelectValue placeholder="Select a chain" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="algorand">🔵 Algorand</SelectItem>
              <SelectItem value="solana">🟣 Solana</SelectItem>
              <SelectItem value="ethereum">🔷 Ethereum</SelectItem>
            </SelectContent>
          </Select>
          <p className="mt-2 text-sm text-muted-foreground">Selected: {value}</p>
        </div>

        <div>
          <label className="block text-sm font-medium mb-2">Disabled</label>
          <Select disabled>
            <SelectTrigger className="w-[200px]">
              <SelectValue placeholder="Select a chain" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="algorand">Algorand</SelectItem>
              <SelectItem value="solana">Solana</SelectItem>
              <SelectItem value="ethereum">Ethereum</SelectItem>
            </SelectContent>
          </Select>
        </div>
      </div>
    </div>
  )
}