// Unstyled default slots; hosts swap any of them for their design system. See `src/react/AGENTS.md` §slots.
import { useId } from "react";
import type { ChangeEvent, ComponentType, ReactNode } from "react";

export interface ButtonSlotProps {
  children?: ReactNode;
  onClick?: () => void;
  disabled?: boolean;
  type?: "button" | "submit";
  /** `primary` for the step's main action, `secondary` for the rest. */
  variant?: "primary" | "secondary";
  className?: string;
}

export interface InputSlotProps {
  id: string;
  value: string;
  onChange: (event: ChangeEvent<HTMLInputElement>) => void;
  inputMode?: "decimal" | "numeric" | "text";
  placeholder?: string;
  disabled?: boolean;
  "aria-invalid"?: boolean;
  "aria-describedby"?: string;
  className?: string;
}

export interface CardSlotProps {
  title?: ReactNode;
  description?: ReactNode;
  children?: ReactNode;
  className?: string;
}

export interface AlertSlotProps {
  /** `error`/`warning` render as `role="alert"`, the others as `role="status"`. */
  tone: "info" | "success" | "warning" | "error";
  children?: ReactNode;
  className?: string;
}

/** The four building blocks every money component renders through. */
export interface MoneySlots {
  Button: ComponentType<ButtonSlotProps>;
  Input: ComponentType<InputSlotProps>;
  Card: ComponentType<CardSlotProps>;
  Alert: ComponentType<AlertSlotProps>;
}

function DefaultButton({ children, onClick, disabled, type, variant, className }: ButtonSlotProps) {
  return (
    <button type={type ?? "button"} onClick={onClick} disabled={disabled} className={className} data-variant={variant ?? "primary"}>
      {children}
    </button>
  );
}

function DefaultInput(props: InputSlotProps) {
  return <input {...props} />;
}

function DefaultCard({ title, description, children, className }: CardSlotProps) {
  const headingId = useId();
  return (
    <section className={className} aria-labelledby={title ? headingId : undefined}>
      {title ? <h3 id={headingId}>{title}</h3> : null}
      {description ? <div>{description}</div> : null}
      {children}
    </section>
  );
}

function DefaultAlert({ tone, children, className }: AlertSlotProps) {
  return (
    <div role={tone === "error" || tone === "warning" ? "alert" : "status"} data-tone={tone} className={className}>
      {children}
    </div>
  );
}

export const defaultMoneySlots: MoneySlots = { Button: DefaultButton, Input: DefaultInput, Card: DefaultCard, Alert: DefaultAlert };
