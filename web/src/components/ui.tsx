import { useEffect, type ReactNode } from 'react'
import { X } from 'lucide-react'
import clsx from 'clsx'

export function Button({
  children,
  variant = 'ghost',
  size = 'md',
  className,
  ...rest
}: {
  children: ReactNode
  variant?: 'primary' | 'ghost' | 'outline' | 'danger'
  size?: 'sm' | 'md'
} & React.ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button
      {...rest}
      className={clsx(
        'inline-flex items-center gap-1.5 rounded-md font-medium transition-colors select-none',
        'disabled:opacity-40 disabled:cursor-not-allowed',
        size === 'sm' ? 'px-2 py-1 text-[12px]' : 'px-3 py-1.5 text-[13px]',
        variant === 'primary' && 'text-white',
        variant === 'ghost' && 'hover:bg-[var(--raised)]',
        variant === 'outline' && 'border hover:bg-[var(--raised)]',
        variant === 'danger' && 'hover:bg-[color-mix(in_srgb,var(--st-miss)_18%,transparent)]',
        className,
      )}
      style={{
        background: variant === 'primary' ? 'var(--accent)' : undefined,
        borderColor: variant === 'outline' ? 'var(--line)' : undefined,
        color: variant === 'danger' ? 'var(--st-miss)' : undefined,
        ...rest.style,
      }}
    >
      {children}
    </button>
  )
}

export function Panel({
  children,
  className,
  ...rest
}: { children: ReactNode } & React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      {...rest}
      className={clsx('rounded-lg border', className)}
      style={{ background: 'var(--surface)', borderColor: 'var(--line)', ...rest.style }}
    >
      {children}
    </div>
  )
}

export function Field({
  label,
  hint,
  children,
  className,
}: {
  label: string
  hint?: string
  children: ReactNode
  className?: string
}) {
  return (
    <label className={clsx('flex flex-col gap-1', className)}>
      <span className="text-[11px] font-medium uppercase tracking-wide" style={{ color: 'var(--text-3)' }}>
        {label}
      </span>
      {children}
      {hint && (
        <span className="text-[11px]" style={{ color: 'var(--text-3)' }}>
          {hint}
        </span>
      )}
    </label>
  )
}

export function Input(props: React.InputHTMLAttributes<HTMLInputElement>) {
  return (
    <input
      {...props}
      className={clsx(
        'rounded-md border px-2.5 py-1.5 text-[13px] outline-none transition-colors',
        'placeholder:text-[var(--text-3)]',
        props.className,
      )}
      style={{ background: 'var(--page)', borderColor: 'var(--line)', ...props.style }}
    />
  )
}

export function Select(props: React.SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <select
      {...props}
      className={clsx('rounded-md border px-2 py-1.5 text-[13px] outline-none', props.className)}
      style={{ background: 'var(--page)', borderColor: 'var(--line)', ...props.style }}
    />
  )
}

export function Toggle({
  checked,
  onChange,
  label,
  hint,
}: {
  checked: boolean
  onChange: (v: boolean) => void
  label: string
  hint?: string
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className="flex w-full items-start gap-2.5 rounded-md px-2 py-1.5 text-left transition-colors hover:bg-[var(--raised)]"
    >
      <span
        className="mt-0.5 flex h-4 w-7 shrink-0 items-center rounded-full p-0.5 transition-colors"
        style={{ background: checked ? 'var(--accent)' : 'var(--line)' }}
      >
        <span
          className="h-3 w-3 rounded-full bg-white transition-transform"
          style={{ transform: checked ? 'translateX(12px)' : 'none' }}
        />
      </span>
      <span className="min-w-0">
        <span className="block text-[12.5px]">{label}</span>
        {hint && (
          <span className="block text-[11px] leading-snug" style={{ color: 'var(--text-3)' }}>
            {hint}
          </span>
        )}
      </span>
    </button>
  )
}

export function Modal({
  title,
  subtitle,
  onClose,
  children,
  footer,
  width = 620,
}: {
  title: string
  subtitle?: string
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
  width?: number
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div
      className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto p-6"
      style={{ background: 'rgba(0,0,0,0.55)' }}
      onMouseDown={(e) => e.target === e.currentTarget && onClose()}
    >
      <div
        className="ss-fade my-auto w-full rounded-xl border shadow-2xl"
        style={{ background: 'var(--surface)', borderColor: 'var(--line)', maxWidth: width }}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div
          className="flex items-start justify-between gap-4 border-b px-5 py-3.5"
          style={{ borderColor: 'var(--line)' }}
        >
          <div>
            <h2 className="text-[15px] font-semibold">{title}</h2>
            {subtitle && (
              <p className="mt-0.5 text-[12px]" style={{ color: 'var(--text-2)' }}>
                {subtitle}
              </p>
            )}
          </div>
          <Button size="sm" onClick={onClose} aria-label="Close">
            <X size={15} />
          </Button>
        </div>

        <div className="px-5 py-4">{children}</div>

        {footer && (
          <div
            className="flex items-center justify-end gap-2 border-t px-5 py-3"
            style={{ borderColor: 'var(--line)' }}
          >
            {footer}
          </div>
        )}
      </div>
    </div>
  )
}

export function Spinner({ size = 14 }: { size?: number }) {
  return (
    <span
      className="ss-spin inline-block rounded-full border-2 border-current border-t-transparent align-[-2px]"
      style={{ width: size, height: size, opacity: 0.7 }}
    />
  )
}

export function EmptyState({
  icon,
  title,
  body,
  action,
}: {
  icon?: ReactNode
  title: string
  body?: string
  action?: ReactNode
}) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-3 px-8 text-center">
      {icon && <div style={{ color: 'var(--text-3)' }}>{icon}</div>}
      <div>
        <div className="text-[14px] font-medium">{title}</div>
        {body && (
          <div className="mt-1 max-w-md text-[12.5px] leading-relaxed" style={{ color: 'var(--text-2)' }}>
            {body}
          </div>
        )}
      </div>
      {action}
    </div>
  )
}
