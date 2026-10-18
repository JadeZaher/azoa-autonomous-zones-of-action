'use client'

import { useMemo, useState } from 'react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Button } from '@/components/ui/button'
import { Separator } from '@/components/ui/separator'
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger,
} from '@/components/ui/dialog'
import { useAzoa } from '@/lib/azoa-context'
import { userFacingMessage } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { ResultDisplay } from '@/components/shared/result-display'
import { JsonViewer } from '@/components/shared/json-viewer'

type ProfileFields = { username: string; email: string; firstName: string; lastName: string; title: string }

function profileFields(user: unknown): ProfileFields {
  const fields = (user ?? {}) as Partial<Record<keyof ProfileFields, unknown>>
  const text = (value: unknown) => (typeof value === 'string' ? value : '')
  return { username: text(fields.username), email: text(fields.email), firstName: text(fields.firstName), lastName: text(fields.lastName), title: text(fields.title) }
}

export default function UsersPage() {
  const { user, userId, logout, refreshProfile } = useAzoa()

  // The form is the server profile plus the person's edits, so a profile that arrives after mount is shown as-is.
  const saved = useMemo(() => profileFields(user), [user])
  const [edits, setEdits] = useState<Partial<ProfileFields>>({})
  const form: ProfileFields = { ...saved, ...edits }

  const [saving, setSaving] = useState(false)
  const [saveResult, setSaveResult] = useState<{ ok: boolean; data?: unknown; message?: string } | null>(null)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [jsonOpen, setJsonOpen] = useState(false)

  const handleChange = (field: keyof ProfileFields) => (e: React.ChangeEvent<HTMLInputElement>) => {
    setEdits(prev => ({ ...prev, [field]: e.target.value }))
  }

  const handleSave = async () => {
    if (!userId) return
    setSaving(true)
    setSaveResult(null)
    try {
      // UserUpdateModel treats an absent field as "unchanged": send only what was edited, so an untouched
      // wallet handle or blank email is never re-validated.
      const changes = Object.fromEntries(
        (Object.keys(form) as (keyof ProfileFields)[]).filter(field => form[field] !== saved[field]).map(field => [field, form[field]]),
      ) as Partial<ProfileFields>
      if (Object.keys(changes).length === 0) {
        setSaveResult({ ok: true, message: 'Nothing to save.' })
        return
      }
      const result = await azoa.api.updateUser(userId, changes)
      if (isOk(result)) {
        await refreshProfile()
        setEdits({})
        setSaveResult({ ok: true, data: result.value, message: 'Profile updated.' })
      } else {
        setSaveResult({ ok: false, message: userFacingMessage(result.error) })
      }
    } catch (err) {
      setSaveResult({ ok: false, message: err instanceof Error ? err.message : 'Unknown error' })
    } finally {
      setSaving(false)
    }
  }

  const handleDelete = async () => {
    setDeleting(true)
    await logout()
    setDeleting(false)
    setDeleteOpen(false)
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">User Profile</h1>
        <p className="text-sm text-muted-foreground">Manage your AZOA identity</p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="text-sm">Profile</CardTitle>
          <CardDescription>Update your user details.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="username">Username</Label>
              <Input id="username" value={form.username} onChange={handleChange('username')} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="email">Email</Label>
              <Input id="email" type="email" value={form.email} onChange={handleChange('email')} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="firstName">First Name</Label>
              <Input id="firstName" value={form.firstName} onChange={handleChange('firstName')} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="lastName">Last Name</Label>
              <Input id="lastName" value={form.lastName} onChange={handleChange('lastName')} />
            </div>
            <div className="space-y-1.5 sm:col-span-2">
              <Label htmlFor="title">Title</Label>
              <Input id="title" value={form.title} onChange={handleChange('title')} placeholder="e.g. Guardian of the AZOA" />
            </div>
          </div>
          <Button onClick={handleSave} disabled={saving || !userId} size="sm">
            {saving ? 'Saving…' : 'Save changes'}
          </Button>
          {saveResult && <ResultDisplay result={saveResult.data} isError={!saveResult.ok} message={saveResult.message} />}
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="pb-3">
          <div className="flex items-center justify-between">
            <CardTitle className="text-sm">Raw Data</CardTitle>
            <Button variant="ghost" size="sm" onClick={() => setJsonOpen(v => !v)}>
              {jsonOpen ? 'Hide' : 'Show'}
            </Button>
          </div>
        </CardHeader>
        {jsonOpen && (
          <CardContent>
            <div className="rounded-md bg-muted/50 p-3 text-xs font-mono">
              <JsonViewer data={user} />
            </div>
          </CardContent>
        )}
      </Card>

      <Separator />

      <Card className="border-destructive/30">
        <CardHeader>
          <CardTitle className="text-sm text-destructive">Danger Zone</CardTitle>
          <CardDescription>Permanently delete your account.</CardDescription>
        </CardHeader>
        <CardContent>
          <Dialog open={deleteOpen} onOpenChange={setDeleteOpen}>
            <DialogTrigger render={<Button variant="destructive" size="sm">Delete account</Button>} />
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Delete Account</DialogTitle>
                <DialogDescription>This cannot be undone.</DialogDescription>
              </DialogHeader>
              <DialogFooter>
                <Button variant="outline" onClick={() => setDeleteOpen(false)} disabled={deleting}>Cancel</Button>
                <Button variant="destructive" onClick={handleDelete} disabled={deleting}>
                  {deleting ? 'Deleting…' : 'Delete'}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </CardContent>
      </Card>
    </div>
  )
}
