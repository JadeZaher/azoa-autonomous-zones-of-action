import type { KycProviderProfileResponse } from '@/lib/operator-contracts'

export function isProviderReady(profile: Pick<KycProviderProfileResponse, 'enabled' | 'available' | 'readinessCode'>): boolean {
  return profile.enabled && profile.available && profile.readinessCode === 'READY'
}

export function humanizeReadiness(code: string): string {
  if (!code) return 'Readiness not reported'
  return code.replace(/[_-]+/g, ' ').replace(/\b\w/g, (letter) => letter.toUpperCase())
}
