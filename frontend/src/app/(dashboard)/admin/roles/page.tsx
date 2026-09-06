'use client'

// user-dapp-rbac operator console — assign an user's DApp role (PUT /api/user/{id}/dapp-role).
// The operator-bootstrap surface: an operator promotes the first dapp:manager here.
import { useState, useCallback } from 'react'
import { azoa, isOk } from '@/lib/azoa'
import type { DappRole, UserResponse } from 'azoa-sdk'
import { DAPP_ROLES } from 'azoa-sdk'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import { ErrorBanner } from '@/components/shared/error-banner'
import { ShieldCheck, UserCog, Info, CheckCircle2 } from 'lucide-react'

// Human-facing labels for the three assignable DApp roles (mirrors Core/AzoaDappRoles.cs).
const ROLE_LABELS: Record<DappRole, string> = {
  'dapp:user': 'User — baseline access, no authoring',
  'dapp:developer': 'Developer — may author dapps/actions',
  'dapp:manager': 'Manager — may author + assign developer/user roles',
}

interface AssignSuccess {
  userId: string
  role: DappRole
  user: UserResponse
}

export default function RolesPage() {
  useAzoaAuth()
  const [userId, setUserId] = useState('')
  const [role, setRole] = useState<DappRole>('dapp:developer')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [success, setSuccess] = useState<AssignSuccess | null>(null)

  const canSubmit = userId.trim().length > 0 && !loading

  const assign = useCallback(async () => {
    setLoading(true)
    setError(null)
    setSuccess(null)
    try {
      const result = await azoa.api.assignDappRole(userId.trim(), role)
      if (isOk(result)) {
        setSuccess({ userId: userId.trim(), role, user: result.value })
      } else {
        // Map the backend's status-coded envelope to demo-legible copy.
        const msg = result.error.message ?? ''
        if (/not found/i.test(msg)) {
          setError('User not found. Check the user id and try again.')
        } else if (/forbidden|403|not authorized|unauthorized/i.test(msg)) {
          setError('You are not authorized to assign this role. An operator can assign any role; a manager can assign only developer/user.')
        } else {
          setError(msg || 'Failed to assign role.')
        }
      }
    } catch (err) {
      // assertUuid / client-side allowlist throws synchronously for a malformed id or role.
      setError(err instanceof Error ? err.message : 'Failed to assign role.')
    } finally {
      setLoading(false)
    }
  }, [userId, role])

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">DApp Roles</h1>
        <p className="text-sm text-muted-foreground">
          Assign an user&apos;s DApp role — the RBAC surface for granting a team member access
        </p>
      </div>

      <Card className="border-primary/30 bg-primary/5">
        <CardHeader className="pb-3">
          <div className="flex items-center gap-2">
            <Info className="h-5 w-5 shrink-0 text-primary" />
            <CardTitle className="text-sm">Operator bootstrap</CardTitle>
          </div>
          <CardDescription>
            An <strong>operator</strong> (JWT <code className="rounded bg-muted px-1 py-0.5 font-mono">operator:admin</code>) can
            assign <em>any</em> of the three roles, including <strong>manager</strong> — this is how the very first
            manager is promoted. A <strong>manager</strong> can then grant only <code className="rounded bg-muted px-1 py-0.5 font-mono">developer</code>{' '}
            and <code className="rounded bg-muted px-1 py-0.5 font-mono">user</code>. Ordinary users and developers cannot assign roles.
            <br />
            <span className="text-xs">You must be signed in as an operator (or manager) for this to succeed.</span>
          </CardDescription>
        </CardHeader>
      </Card>

      <Card>
        <CardHeader className="pb-3">
          <div className="flex items-center gap-2">
            <UserCog className="h-5 w-5 shrink-0 text-foreground" />
            <CardTitle className="text-sm">Assign a role</CardTitle>
          </div>
          <CardDescription>Enter the target user id, pick a role, and assign.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="user-id">User id</Label>
              <Input
                id="user-id"
                value={userId}
                onChange={(e) => setUserId(e.target.value)}
                placeholder="00000000-0000-0000-0000-000000000000"
                autoComplete="off"
                spellCheck={false}
                className="font-mono text-xs"
              />
              <p className="text-xs text-muted-foreground">The user whose role you are setting (a UUID).</p>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="role-select">Role</Label>
              <Select value={role} onValueChange={(v) => setRole(v as DappRole)}>
                <SelectTrigger id="role-select">
                  <SelectValue placeholder="Select a role" />
                </SelectTrigger>
                <SelectContent>
                  {DAPP_ROLES.map((r) => (
                    <SelectItem key={r} value={r}>
                      <span className="font-mono text-xs">{r}</span>
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <p className="text-xs text-muted-foreground">{ROLE_LABELS[role]}</p>
            </div>
          </div>

          {error && <ErrorBanner message={error} />}

          <div className="flex justify-end">
            <Button disabled={!canSubmit} onClick={assign}>
              {loading ? 'Assigning…' : 'Assign role'}
            </Button>
          </div>
        </CardContent>
      </Card>

      {success && (
        <Card className="border-primary/40 bg-primary/5">
          <CardHeader className="pb-3">
            <div className="flex items-center gap-2">
              <CheckCircle2 className="h-5 w-5 shrink-0 text-primary" />
              <CardTitle className="text-sm">Role assigned</CardTitle>
            </div>
            <CardDescription>
              {success.user.username || success.user.email || 'The user'} now has the role below.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <ShieldCheck className="h-4 w-4 shrink-0 text-primary" />
              <span className="text-muted-foreground">New role:</span>
              <Badge variant="secondary" className="font-mono">{success.role}</Badge>
            </div>
            <Separator />
            <div className="grid gap-1 text-xs text-muted-foreground sm:grid-cols-2">
              <div><span className="uppercase tracking-wider">User</span> <span className="font-mono">{success.user.id}</span></div>
              {success.user.email && (
                <div><span className="uppercase tracking-wider">Email</span> {success.user.email}</div>
              )}
            </div>
          </CardContent>
        </Card>
      )}
    </div>
  )
}
