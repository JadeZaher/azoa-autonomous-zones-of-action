'use client'

import { Copy, ClipboardPaste, Redo2, Undo2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import {
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuLabel,
  DropdownMenuItem,
  DropdownMenuSeparator,
} from '@/components/ui/dropdown-menu'
import { ACTION_PRESETS, presetIsLoadable, type ActionPreset } from '../presets'

interface CanvasToolbarProps {
  nodeCount: number
  onLoadPreset: (preset: ActionPreset) => void
  onAutoLayout: () => void
  onClear: () => void
  onUndo: () => void
  onRedo: () => void
  canUndo: boolean
  canRedo: boolean
  onCopy: () => void
  onPaste: () => void
  canCopy: boolean
  canPaste: boolean
}

/** One icon button with a native hover tooltip (`title`) — the toolbar's icon-only actions share this shape. */
function ToolbarIconButton({
  label,
  shortcut,
  disabled,
  onClick,
  children,
}: {
  label: string
  shortcut: string
  disabled?: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <Button
      size="icon-sm"
      variant="outline"
      disabled={disabled}
      onClick={onClick}
      aria-label={label}
      title={`${label} (${shortcut})`}
    >
      {children}
    </Button>
  )
}

/** Floating canvas toolbar: preset loader, undo/redo, copy/paste, auto-layout, clear. */
export function CanvasToolbar({
  nodeCount,
  onLoadPreset,
  onAutoLayout,
  onClear,
  onUndo,
  onRedo,
  canUndo,
  canRedo,
  onCopy,
  onPaste,
  canCopy,
  canPaste,
}: CanvasToolbarProps) {
  return (
    <div className="absolute right-2 top-2 z-10 flex gap-1">
      <ToolbarIconButton label="Undo" shortcut="Ctrl+Z" disabled={!canUndo} onClick={onUndo}>
        <Undo2 />
      </ToolbarIconButton>
      <ToolbarIconButton label="Redo" shortcut="Ctrl+Shift+Z" disabled={!canRedo} onClick={onRedo}>
        <Redo2 />
      </ToolbarIconButton>
      <ToolbarIconButton label="Copy selection" shortcut="Ctrl+C" disabled={!canCopy} onClick={onCopy}>
        <Copy />
      </ToolbarIconButton>
      <ToolbarIconButton label="Paste" shortcut="Ctrl+V" disabled={!canPaste} onClick={onPaste}>
        <ClipboardPaste />
      </ToolbarIconButton>
      <DropdownMenu>
        <DropdownMenuTrigger className="inline-flex h-8 items-center rounded-md border bg-background px-3 text-sm font-medium transition-colors hover:bg-accent">
          Presets ▾
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-72">
          {/*
            The label MUST stay inside DropdownMenuGroup. Base UI's `Menu.GroupLabel`
            reads `MenuGroupRootContext` and THROWS when it is missing — in production
            builds too — which takes the whole React tree down the first time a user
            opens this menu.
          */}
          <DropdownMenuGroup>
            <DropdownMenuLabel>Load a starter flow</DropdownMenuLabel>
            <DropdownMenuSeparator />
            {ACTION_PRESETS.map((preset) => {
              const loadable = presetIsLoadable(preset)
              return (
                <DropdownMenuItem
                  key={preset.id}
                  disabled={!loadable}
                  onClick={() => onLoadPreset(preset)}
                  className="flex-col items-start gap-0.5"
                >
                  <span className="flex w-full items-center gap-1.5 text-xs font-medium">
                    {preset.name}
                    {preset.requiresChain && (
                      <Badge variant="outline" className="text-[8px]">on-chain</Badge>
                    )}
                  </span>
                  <span className="text-[10px] text-muted-foreground">{preset.description}</span>
                </DropdownMenuItem>
              )
            })}
          </DropdownMenuGroup>
        </DropdownMenuContent>
      </DropdownMenu>
      <Button size="sm" variant="outline" onClick={onAutoLayout} disabled={nodeCount === 0}>
        Auto-layout
      </Button>
      <Button size="sm" variant="outline" onClick={onClear} disabled={nodeCount === 0}>
        Clear
      </Button>
    </div>
  )
}
