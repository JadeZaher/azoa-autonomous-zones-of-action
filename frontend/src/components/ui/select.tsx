"use client"

import * as React from "react"
import { ChevronDownIcon, CheckIcon } from "lucide-react"

import { cn } from "@/lib/utils"

/**
 * Self-contained Select primitives (no Base UI / shadcn dependency).
 *
 * Why this exists: the previous wrapper was the shadcn registry component
 * built for Base UI + Tailwind v4. Base UI's Select.Item only commits a
 * selection when the item is in its internal "highlighted" state, which the
 * registry styles never wired up under Tailwind v3 — so clicks never
 * registered. This implementation uses a plain button + controlled popup so
 * clicks always work, while preserving the original export surface so callers
 * don't change.
 */

type SelectContextValue = {
  value: string
  open: boolean
  disabled: boolean
  setOpen: (open: boolean) => void
  select: (value: string) => void
  registerLabel: (value: string, label: React.ReactNode) => void
  labels: Record<string, React.ReactNode>
  triggerId: string
}

const SelectContext = React.createContext<SelectContextValue | null>(null)

function useSelectContext(component: string) {
  const ctx = React.useContext(SelectContext)
  if (!ctx) {
    throw new Error(`<${component}> must be used within <Select>`)
  }
  return ctx
}

type SelectProps = {
  value?: string | null
  defaultValue?: string | null
  onValueChange?: (value: string) => void
  disabled?: boolean
  /** Identifies the field when used inside a form (renders a hidden input). */
  name?: string
  /** Requires a value before form submission (validated via the hidden input). */
  required?: boolean
  children?: React.ReactNode
}

function Select({
  value: controlledValue,
  defaultValue = "",
  onValueChange,
  disabled = false,
  name,
  required = false,
  children,
}: SelectProps) {
  const isControlled = controlledValue !== undefined
  const [uncontrolled, setUncontrolled] = React.useState(defaultValue ?? "")
  const value = (isControlled ? controlledValue : uncontrolled) ?? ""

  const [open, setOpen] = React.useState(false)
  const [labels, setLabels] = React.useState<Record<string, React.ReactNode>>(
    {}
  )
  const containerRef = React.useRef<HTMLDivElement>(null)
  const triggerId = React.useId()

  const select = React.useCallback(
    (next: string) => {
      if (!isControlled) setUncontrolled(next)
      onValueChange?.(next)
      setOpen(false)
    },
    [isControlled, onValueChange]
  )

  const registerLabel = React.useCallback(
    (val: string, label: React.ReactNode) => {
      setLabels((prev) =>
        prev[val] === label ? prev : { ...prev, [val]: label }
      )
    },
    []
  )

  const handleOpen = React.useCallback(
    (next: boolean) => {
      if (disabled) return
      setOpen(next)
    },
    [disabled]
  )

  // Close on outside pointer / Escape.
  React.useEffect(() => {
    if (!open) return
    function onPointerDown(e: PointerEvent) {
      if (!containerRef.current?.contains(e.target as Node)) setOpen(false)
    }
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false)
    }
    document.addEventListener("pointerdown", onPointerDown)
    document.addEventListener("keydown", onKeyDown)
    return () => {
      document.removeEventListener("pointerdown", onPointerDown)
      document.removeEventListener("keydown", onKeyDown)
    }
  }, [open])

  const ctx = React.useMemo<SelectContextValue>(
    () => ({
      value,
      open,
      disabled,
      setOpen: handleOpen,
      select,
      registerLabel,
      labels,
      triggerId,
    }),
    [value, open, disabled, handleOpen, select, registerLabel, labels, triggerId]
  )

  return (
    <SelectContext.Provider value={ctx}>
      <div ref={containerRef} className="relative inline-block">
        {name ? (
          <input
            type="text"
            name={name}
            value={value}
            required={required}
            tabIndex={-1}
            aria-hidden="true"
            readOnly
            className="sr-only absolute h-0 w-0 opacity-0"
            onFocus={(e) => {
              // Bounce focus to the trigger if the browser focuses the
              // hidden input (e.g. on failed form validation).
              ;(
                e.currentTarget.parentElement?.querySelector(
                  '[data-slot="select-trigger"]'
                ) as HTMLElement | null
              )?.focus()
            }}
          />
        ) : null}
        {children}
      </div>
    </SelectContext.Provider>
  )
}

function SelectGroup({
  className,
  ...props
}: React.ComponentProps<"div">) {
  return (
    <div data-slot="select-group" className={cn("p-1", className)} {...props} />
  )
}

function SelectValue({
  className,
  placeholder,
  ...props
}: React.ComponentProps<"span"> & { placeholder?: React.ReactNode }) {
  const { value, labels } = useSelectContext("SelectValue")
  const hasValue = value !== "" && value != null
  const content = hasValue ? labels[value] ?? value : placeholder

  return (
    <span
      data-slot="select-value"
      data-placeholder={!hasValue ? "" : undefined}
      className={cn(
        "flex flex-1 items-center gap-1.5 text-left line-clamp-1",
        !hasValue && "text-muted-foreground",
        className
      )}
      {...props}
    >
      {content}
    </span>
  )
}

function SelectTrigger({
  className,
  size = "default",
  children,
  ...props
}: React.ComponentProps<"button"> & { size?: "sm" | "default" }) {
  const { open, setOpen, disabled, triggerId } = useSelectContext(
    "SelectTrigger"
  )

  return (
    <button
      type="button"
      id={triggerId}
      role="combobox"
      aria-haspopup="listbox"
      aria-expanded={open}
      data-slot="select-trigger"
      data-size={size}
      data-state={open ? "open" : "closed"}
      disabled={disabled}
      onClick={() => setOpen(!open)}
      className={cn(
        "flex w-fit items-center justify-between gap-1.5 rounded-lg border border-input bg-transparent py-2 pr-2 pl-2.5 text-sm whitespace-nowrap transition-colors outline-none select-none",
        "focus-visible:border-ring focus-visible:ring-2 focus-visible:ring-ring/50",
        "disabled:cursor-not-allowed disabled:opacity-50",
        "data-[size=default]:h-8 data-[size=sm]:h-7 data-[size=sm]:rounded-md",
        "dark:bg-input/30 dark:hover:bg-input/50",
        "[&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
        className
      )}
      {...props}
    >
      {children}
      <ChevronDownIcon
        className={cn(
          "size-4 shrink-0 text-muted-foreground transition-transform pointer-events-none",
          open && "rotate-180"
        )}
      />
    </button>
  )
}

type SelectContentProps = React.ComponentProps<"div"> & {
  // Accepted for API compatibility with the previous Base UI wrapper.
  side?: string
  sideOffset?: number
  align?: string
  alignOffset?: number
  alignItemWithTrigger?: boolean
}

function SelectContent({
  className,
  children,
  // positioning props intentionally ignored (kept for API compatibility)
  side: _side,
  sideOffset: _sideOffset,
  align: _align,
  alignOffset: _alignOffset,
  alignItemWithTrigger: _alignItemWithTrigger,
  ...props
}: SelectContentProps) {
  const { open } = useSelectContext("SelectContent")

  return (
    <div
      data-slot="select-content"
      data-state={open ? "open" : "closed"}
      role="listbox"
      hidden={!open}
      className={cn(
        "absolute left-0 top-full z-50 mt-1 max-h-72 min-w-[8rem] w-max overflow-y-auto rounded-lg bg-popover p-1 text-popover-foreground shadow-md ring-1 ring-foreground/10",
        !open && "hidden",
        className
      )}
      {...props}
    >
      {children}
    </div>
  )
}

function SelectLabel({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="select-label"
      className={cn("px-1.5 py-1 text-xs text-muted-foreground", className)}
      {...props}
    />
  )
}

type SelectItemProps = Omit<React.ComponentProps<"div">, "onSelect"> & {
  value: string
  disabled?: boolean
}

function SelectItem({
  className,
  children,
  value,
  disabled = false,
  ...props
}: SelectItemProps) {
  const { value: selected, select, registerLabel } = useSelectContext(
    "SelectItem"
  )
  const isSelected = selected === value

  React.useEffect(() => {
    registerLabel(value, children)
  }, [value, children, registerLabel])

  return (
    <div
      data-slot="select-item"
      role="option"
      aria-selected={isSelected}
      data-state={isSelected ? "checked" : "unchecked"}
      data-disabled={disabled ? "" : undefined}
      onClick={() => {
        if (!disabled) select(value)
      }}
      className={cn(
        "relative flex w-full cursor-pointer items-center gap-1.5 rounded-md py-1.5 pr-8 pl-1.5 text-sm outline-none select-none",
        "hover:bg-accent hover:text-accent-foreground",
        isSelected && "bg-accent/60",
        disabled && "pointer-events-none opacity-50",
        "[&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
        className
      )}
      {...props}
    >
      <span className="flex flex-1 shrink-0 items-center gap-2 whitespace-nowrap">
        {children}
      </span>
      {isSelected && (
        <span className="absolute right-2 flex size-4 items-center justify-center">
          <CheckIcon />
        </span>
      )}
    </div>
  )
}

function SelectSeparator({
  className,
  ...props
}: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="select-separator"
      className={cn("-mx-1 my-1 h-px bg-border", className)}
      {...props}
    />
  )
}

// Scroll buttons are no-ops here (native overflow scrolling is used).
// Kept for API compatibility with existing imports.
function SelectScrollUpButton(_props: React.ComponentProps<"div">) {
  return null
}

function SelectScrollDownButton(_props: React.ComponentProps<"div">) {
  return null
}

export {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectScrollDownButton,
  SelectScrollUpButton,
  SelectSeparator,
  SelectTrigger,
  SelectValue,
}
